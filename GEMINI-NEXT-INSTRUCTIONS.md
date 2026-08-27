# Gemini continuation instructions

This is the continuation brief for a fresh Gemini agent working on AAEmu. It is
an evidence handoff, not a claim that MCP replaces the real client, packet, DB,
restart, scaling, or human gates.

## 1. Audit result and checkpoint

The handoff documents were audited against a clean worktree created from
`origin/develop` on 2026-08-27. At audit start the remote branch resolved to:

```text
origin/develop = 07230fe5d3b471c5b6c8ec23b4ab7805b7f57453
```

`1638b007c` is the historical feature commit that added the five actor routes;
it is not the current branch head. `6d9ae9f50` recorded that expansion in the
status/scorecard/capability documents, and `07230fe5d` added the later roadmap
record. The older `241d3e34d` Mail/PB-007 reconciliation point and
`7e109d550` asset-missing MCP smoke are historical. The checked-in integrated
benchmark report was run from the earlier `12ff5b504` base and its 19-tool
protocol count is historical; do not use it as the current catalog verdict.

The current recorded state is:

- **24 MCP action tools** are exposed by `AAEmu.BotControlMcp`; management tools
  remain on the separate `AAEmu.BotControl` sidecar.
- The five newly authenticated actor routes are
  `POST /api/actors/discover_quests`,
  `POST /api/actors/discover_self_quests`,
  `POST /api/actors/interact_with`, `POST /api/actors/talk`, and
  `POST /api/actors/equip`.
- The 24-tool smoke passed. Focused route/MCP/queue validation is **51/51**:
  `BotActionControllerRouteTests` 2/2, `BotControlActionMcpTests` 33/33, and
  `BotActionCommandQueueTests` 16/16. Release builds and the full recorded gate
  (2486 total / 2485 succeeded / 0 failed / 1 skipped) are historical evidence;
  this documentation review does not rerun them.
- The live recorded MCP+DB benchmark is **PASS** for authenticated management
  provisioning plus actor `observe`/`move`/`discover_self_quests`, terminal
  `action_status`, follow-up `trace`, and an independent MySQL character-row
  cross-check. It does not prove a client packet transition, restart parity,
  broad navigation, scaling, or human feel.
- The client-wire leg is bounded by lifecycle: managed `HeadlessBot` accounts
  are blocked from public client login and are not `BotNetworkSession` actors.
  The attempted bridge check therefore reports no active networked session. A
  future wire proof needs a separately authenticated, client-login-allowed
  ordinary account; never relabel the managed headless DB row as wire evidence.
- `H` remains human-only. MCP, scripted actors, headless sessions, rigs, and
  bots can prove functional/proxy behavior but cannot promote a human-feel
  score.

Authoritative detail is in `AAEmu.BotControlMcp/MCP-ACTION-MATRIX.md`,
`AAEmu.BotControlMcp/README.md`, `STATUS.md`, `ROADMAP.md`, `SCORECARD.md`,
the playerbot capability matrix, and the blocker/dossier files. Read those
before changing a claim. Preserve historical reports even when their verdict,
base SHA, or tool count is superseded.

## 2. Safe start: never use the dirty main checkout as a worktree

The existing `/root/aaemu-dev` checkout has user changes and retained survivor
worktrees. Start read-only and branch from the remote into a new temporary
worktree:

```bash
cd /root/aaemu-dev
git status --short --branch
git worktree list --porcelain
git fetch origin develop
BASE="$(git rev-parse origin/develop)"
printf 'origin/develop=%s\n' "$BASE"
TMP="$(mktemp -d /tmp/aaemu-gemini.XXXXXX)"
git worktree add --detach "$TMP" origin/develop
cd "$TMP"
git status --short --branch
git log -8 --oneline --decorate
```

The status in the temporary worktree must be clean before editing. If
`origin/develop` moved, record the new full SHA and re-read this brief and the
source-grounded matrices before proceeding. Do not copy dirty source changes
from `/root/aaemu-dev` or any survivor into the temporary worktree.

Never run any of these against the dirty main checkout or a survivor:

```text
git reset --hard       git clean -fdx       rm -rf .worktrees
 git worktree prune    overwrite/delete a survivor    force-push
```

Inspect a survivor's branch, dirty state, and evidence role before any use. Do
not add `.worktrees/` wholesale. `compact.sqlite3` is canonical reference data
and is **SELECT-only**; never patch, replace, or delete it. Mutable state belongs
in MySQL or an additive metadata schema. Push only the writable fork `origin`
(`joshhmann/AAEmu`); `upstream` is fetch-only and must never receive a branch or
PR.

For this docs-only continuation commit, stage only named files and prove the
staged list before committing:

```bash
git diff --check
git add GEMINI-NEXT-INSTRUCTIONS.md HANDOFF-GEMINI.md
git diff --cached --name-status
git commit -m 'docs(recovery): add Gemini continuation instructions'
git push origin HEAD:develop
git ls-remote origin refs/heads/develop
```

Do not use `git add .`, force-push, or push upstream. Remove only the temporary
worktree after the remote head is verified:

```bash
cd /root/aaemu-dev
git worktree remove "$TMP"
```

## 3. Current MCP contract and evidence loop

The action sidecar is a client-neutral MCP stdio process that sends authenticated
HTTP requests to enqueue-only `/api/actors/*` routes. The current 24 tools are:

```text
observe move interact discover_quests discover_self_quests interact_with talk
equip accept_quest turn_in_quest loot use_item mount move_to_unit stop target cast
dismount advance_quest turn_in_doodad auto_turn_in interrupt action_status trace
```

Use the separate management sidecar for `bot_add`, `bot_remove`, `bot_list`,
`bot_relocate`, and `bot_status`; those are intentionally not action tools.
For every action, record the exact request and acknowledgement, poll the
returned `trace_id` with `action_status` until a terminal state, then retrieve
`trace` and assert the observable result. A `Completed` response without the
expected state is a blocker, not success. Do not guess a doodad target: use
`--safe-doodad-obj-id` only for an independently verified safe object.

The reusable driver is:

```bash
AAEMU_BOT_CTRL_URL=http://127.0.0.1:1280 \
AAEMU_BOT_CTRL_TOKEN='<temporary token supplied out-of-band>' \
python3 Scripts/mcp-integrated-e2e-benchmark.py \
  --transcript /tmp/aaemu-mcp-integrated-transcript.jsonl \
  --bridge-port 1260
```

Never commit the token, put it in shared config, or record it in a transcript.
The driver is bounded: it initializes both sidecars, lists tools, provisions and
adopts a bot, runs `observe`, `discover_self_quests`, `move`, status/trace polls,
and optionally exercises `interact_with` only with the safe object argument.
Its bridge result is an independent diagnostic, not managed-bot client-wire
proof.

## 4. Combined workflow for every new actor family

Do the layers in this order; do not collapse them into a single MCP assertion:

1. **MCP actor drives:** authenticate the route, submit one real action, and
   retain the exact JSON-RPC and HTTP request/response.
2. **`action_status` / `trace`:** poll to a terminal state and correlate the
   lifecycle/audit trace with state changes and honest failure reasons.
3. **Direct checks:** run the smallest applicable authenticated E2E and inspect
   ordinary server state. Use SELECT-only MySQL queries for durable rows;
   exercise process restart/reload through the existing E2E stack when
   persistence is part of the contract.
4. **Wire boundary:** for player-facing packet behavior, use an ordinary
   client-login-allowed TCP session and source-grounded packet decoding. The
   managed headless actor cannot satisfy this leg. Do not invent packet IDs,
   offsets, or formulas.
5. **Scaling metrics:** only after behavior is correct, collect a no-bot baseline
   and approved numeric budgets for tick p95/p99, memory, DB writes, queue
   backlog, action latency, and recovery. Default-off machinery must remain
   neutral when unset.
6. **Human QAT:** Josh performs any visual/client/feel or H gate. Record that
   result separately; never infer H from MCP, a bot, a rig, or a wire proxy.

A family is not accepted until its route authentication/binding and MCP schema
mapping are covered, negative/idempotency/lifecycle behavior is covered, one
real MCP scenario passes, and all applicable direct E2E/DB/wire/restart layers
have evidence. Update `STATUS.md`, `ROADMAP.md`, `SCORECARD.md`, and the
capability matrix in the same documentation wave; preserve old reports and
label current, historical, deferred, and human-only evidence.

## 5. Asset-complete local stack

On this host the required external assets are accessible at:

```text
/root/hl-cp-test/ClientData/game_pak
/root/hl-cp-test/Data/compact.sqlite3
```

The pak is a regular file and the SQLite file is present. Treat both as
read-only. If a future host lacks either path, stop at the asset prerequisite
and document the missing path; do not download into the repository or invent a
substitute.

Use an isolated temporary E2E root and link the external data (the link avoids a
23-GB pak copy). Do not use a production root or a survivor root:

```bash
cd /root/aaemu-dev
ASSET_ROOT=/root/hl-cp-test
 test -f "$ASSET_ROOT/ClientData/game_pak"
 test -f "$ASSET_ROOT/Data/compact.sqlite3"
E2E_ROOT="$(mktemp -d /tmp/aaemu-gemini-e2e.XXXXXX)"
mkdir -p "$E2E_ROOT/runtime"
ln -s "$ASSET_ROOT" "$E2E_ROOT/runtime/game-data"
E2E_ROOT="$E2E_ROOT" COMPOSE_PROJECT_NAME=gemini-e2e E2E_BRIDGE=1 \
  ./Scripts/e2e/e2e-boot.sh
E2E_ROOT="$E2E_ROOT" COMPOSE_PROJECT_NAME=gemini-e2e \
  ./Scripts/e2e/e2e-stack.sh status
```

`e2e-boot.sh` publishes/uses the real Login and Game binaries, creates the
isolated MySQL container, copies only the runtime `Data` directory, and
symlinks runtime `ClientData`; it writes local configs with the stack's ports
(Login 1237/1234, Game 1239/1250, bridge 1260, WebApi 1280, DB 3306). Wait for
seeded MySQL data, not merely a container ping. The first-time data-sync form
(`./Scripts/e2e/e2e-boot.sh --provision-data`) is for a canonical remote source;
with the paths above, the explicit temporary `runtime/game-data` link is the
safe local form.

Run the MCP driver only after the Game log reports WebApi on port 1280 and
supply the token out-of-band. Use the existing integration E2E harness for
restart and direct TCP checks; use the repository's targeted test selector only
for the contract being changed. On completion, record logs/transcripts outside
the repository, shut down only this isolated stack, and remove only its temp
root:

```bash
E2E_ROOT="$E2E_ROOT" COMPOSE_PROJECT_NAME=gemini-e2e \
  ./Scripts/e2e/e2e-stack.sh db-down
rm -rf "$E2E_ROOT"   # E2E_ROOT is the mktemp directory created above only
```

## 6. Ordered next work

Do not mass-generate endpoints. For each family, perform archaeology and write
the player-visible contract before implementation. The ordered route backlog is:

1. **Deposit/Withdraw** (money and item bank paths; include `BuildHouse` only
   if a separate reviewed actor route is justified).
2. **Plant/Harvest** (including livestock/farm object legality and observable
   inventory/doodad effects).
3. **Craft** (canonical recipe/skill/labor requirements and completion effects).
4. **Buy/Sell** (real vendor paths and conservation/error behavior).
5. **Pack/vehicle** — `PackPickup`, `PutDown`, `LoadPackOntoVehicle`,
   `DriveVehicle`, `BoardVehicle`, and `UnboardVehicle` only after each route's
   target and persistence/wire contract is reviewed.
6. **Party** — invite, accept, follow, assist; no management alias.
7. **Expedition** — create, invite, accept, leave; server-wide reads are not
   actor actions.
8. **Trade** — offer, put-up, lock/OK; preserve the existing trade-pack and
   payout evidence boundaries.
9. **Auction** — post and buy; include expiry/restart and fee/money invariants.

The matrix also marks `NavigateTo` and `CastAt` deferred because they have no
actor WebApi route. Do not call a contract method MCP-exposed merely because a
headless rig can call it. Plant, Harvest, Craft, party, trade, expeditions,
pack/vehicle, bank/economy, auction, and related newer actions remain deferred
until authenticated enqueue routes and reviewed observable contracts exist.

Parallel to that route work, the required gates are:

- **PB-007 live wire proof:** keep the passing real `Skill.Use`/same-faction
  `ForceAttack` rig and crime evidence. Close the blocker only with a real
  victim-matched, non-immune `SCUnitDamaged` frame on a separately authenticated
  ordinary TCP client, alongside HP/Retribution/crime checks. An immune-tagged
  frame or managed headless actor is insufficient.
- **PB-005 owner decisions:** classify cave/deck/submerged grounding rows only
  with canonical/client evidence and decide ownership of the 733 duplicate
  rows. Do not add a negative-Z clamp, delete duplicates, or alter the
  intentional aerial/water/structure whitelist without an explicit owner
  decision and cited evidence.
- **Six-hour dormancy soak:** retain the sequential-seeding rule and stage a
  no-bot baseline, one bot for 30 minutes, 10 bots for one hour, then 10 bots for
  six hours. Approve numeric p95/p99 tick, memory, DB-write, queue, and recovery
  budgets before calling the soak a pass; a qualitative "no overrun" is not
  evidence.
- **Next gameplay reconstruction slices:** choose one evidence-rich slice such
  as justice trial packet ordering/client capture, mail return client-opcode
  capture, or the navigation route-planner/coarse-travel leg. Use neighboring
  code, packet source, canonical data, DB schema, and client captures; mark
  VERIFIED/STRONGLY_INFERRED/PLAUSIBLE/UNKNOWN. A candidate opcode or formula is
  not a fact until the required capture/source evidence exists.

## 7. Archaeology, blockers, and scorecard discipline

Use Graphify for discovery, not as proof. In the clean worktree:

```bash
cd /root/aaemu-dev
graphify explain "GameplayActor Plant" --graph graphify-out/graph.json
graphify affected "BotActionController" --depth 2
graphify path "GameplayActor" "BotActionController"
graphify query "how does the canonical harvest path persist inventory" \
  --graph graphify-out/graph.json
```

Then open the cited source, packet, data, and dossier lines yourself. After
structural source changes, `graphify update .` refreshes the ignored graph
artifacts; do not commit generated graph output. For data archaeology, build
or query the separate graph only from a copy/read-only view of
`compact.sqlite3`, using `tools/gamedata-graph/build-gamedata-graph.py` and the
query examples in `tools/quest-graph/README.md`; never modify the canonical DB.

When a slice fails, append a blocker in the existing format in
`scorecard-explorations/playerbot-blockers.md`: stable ID, scenario/intended
action, observed versus expected, layer (`BOT-SIDE`/`SERVER`/`DATA`/`UNKNOWN`),
exact command/trace/log/DB or wire evidence, status, and next owner. Keep the
original failure and add dated corrections/addenda; do not rewrite a historical
FAIL into a PASS. Update the related dossier and then update STATUS, ROADMAP,
SCORECARD, and the capability matrix with the same evidence boundary. Human
QAT decisions belong in the owner-controlled QAT packet and do not get inferred
from automated evidence.

## 8. Stop and escalate

Stop and preserve evidence when any of the following occurs:

- the remote head moves unexpectedly, the temporary worktree is dirty before
  editing, or a proposed change would touch the dirty main checkout, survivor
  worktree, generated output, or canonical SQLite;
- a route, packet, formula, coordinate, timer, or client behavior is UNKNOWN
  and no source/data/capture evidence resolves it;
- MCP says `Completed` but status/trace/state, DB, wire, or restart evidence is
  absent or contradictory;
- a failure could be infrastructure, asset, login, seed, or lifecycle rather
  than gameplay, or a managed-headless/client-wire boundary is being blurred;
- a proposed fix is a shortcut (direct DB/Transform/ZoneId/GM mutation, fake
  route, hidden state, management alias, negative grounding clamp, duplicate
  deletion, or silent default-on scaling); or
- a human-only/H decision, client capture, owner ruling, or production deploy is
  required and Josh has not supplied it.

Record the exact blocker and escalation owner, retain all historical evidence,
and do not guess or retry until the evidence contract is repaired.
