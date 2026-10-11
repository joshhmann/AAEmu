# Bounded quest travel stuck recovery

The bot used to treat sideways movement as progress even when it never approached
its waypoint. The existing quest travel retry path had no relocation fallback.
E152 reproduced both failures before changing production code: movement5/6 and
quest19/20 passed, with the new behavioral assertion failing in each class.
The tests pin the character position or alternate sideways positions; they do
not establish the cause of E149 or prove a real navigation obstruction.

The bot now measures its best distance to the current waypoint. Ordinary
bounded lateral nudges and Navigation failure remain. Routed detours can move
away from the final destination while still approaching their current waypoint.
After two distinct stuck quest Move failures, one disclosed local relocation
can target that same current waypoint, within75m and the existing leg envelope.
The same retained active quest instance, owner, world and phase must still match.
A60s cooldown applies across quests; the budget is once per quest instance within
the current runtime generation, not durable across restart.

Recovery refuses dead, combat, autoattacking, party, underwater, attached,
position-disabled and foreign-request cases. The destination requires finite
coordinates inside known world bounds, a region, the same known zone, qualified
nonzero terrain height within3m of the waypoint and dry terrain. Unknown data
fails closed. These guards do not prove an obstacle-free or collision-free landing.
Ordinary Character.SetPosition marks position dirty and updates world visibility;
the existing standstill notification is sent. The actor records
QUEST_STUCK_RECOVERY, verifies the resulting position, and clears the old route
and source-arm state. Old Move requests remain Navigation failures with one audit;
no quest progress, reward, inventory or prerequisite is injected. The next wake
makes a fresh ordinary decision under the retained owner.

If recovery is refused or already used, its route is superseded. NPC-loot travel
retains its existing source-arm exemption; no new three-arm rule was added.
The actual ordinary quest runtime then earns its existing unresolved-target
GiveUp at exactly ten wakes in the blocked rig, releases the owner and leaves
the quest active and uncompleted. Ten further wakes issue no additional Move.
No new GiveUpSink or threshold is introduced.

Main owns two production files, GameplayActor.cs and BotRoamStepExecutor.cs,
and the existing movement-stuck and quest-owner test classes. The policy,
status, scorecard, roadmap and existing dashboard checkpoint describe this
bounded slice. User authorization permits disclosed stuck recovery teleport;
it does not replace ordinary walking acceptance or authoritative quest rules.
No general navigation rewrite, administrator feature, second brain, final remote
source jump, forced dismount, combat reset, synthetic credit, schema/data edits,
merge, push, shared deployment or fresh live allocation is included.

Source checkpoint: 470852f4e60090c646917d1f06a117478ea07ff1 on
fix/quest-stuck-recovery-2026-10-11 in normal clone
/root/aaemu-overnight-quest-20261011. Main develop remains clean
2f93a2fc998704835c856b68ebefaf406b9c2840. Nearby-prey c175b505 and corpse
c23aee47 predecessors remain preserved and unmerged; E130 remains unmerged.
Vibe is off; no Muse handoff. No parent milestone, autonomy grade or H promotion.

Evidence is source and deterministic seeded rig. The fixture seeds quest/loot
relationships, sources, fake clock, terrain1 and dry same-zone metadata. It pins
position to simulate obstruction and explicitly moves one source after recovery
to exercise another ordinary travel decision and the used-budget retirement.
This is not a scripted quest assignment or quest-credit grant. Negative controls
use component seams for terrain, stale state, death/combat, attachment and water;
they do not prove a live scheduler or mounted-player scenario.

Focused commands, from the owned normal clone:

```bash
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --treenode-filter '/*/*/GameplayActorMoveStuckDetectionTests/*'
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --treenode-filter '/*/*/QuestOwnerHandoffTests/*'
```

Movement7/7 and quest51/51 pass, zero failures or skips. The original movement5
and owner19 controls remain. New checks cover oscillation, valid routed detour,
two distinct terminals, one relocation, unchanged quest, ordinary next decision,
earned retirement, no further dispatch, interruption, exact-once audit, duplicate
failure, expired failure window, lateral versus useful approach, stale owner/
quest/status/world/generation, busy request, cooldown, invalid coordinates,
unknown bounds/zone/height, different zone, water and unsafe character state.
Intermediate failing receipts are preserved; fixture corrections are not hidden.
The final independent Sol review is textual, read-only and finds no material
bounded blocker. It does not constitute execution or live acceptance.

Archaeology: read-only MCP list_sources, list_tables and read_file for the two
production paths, plus independent relocation/guard/runtime source reads.
Final inputs and raw output: mcp-final-inputs.jsonl / mcp-final-raw.jsonl.
All four final tools return ok=true, provenance.truncated=false. Catalog evidence
is exact: source_id compact.sqlite3, clone Data/compact.sqlite3 path,
version1.2 r208022,679 tables; canonical MD5
78b3bdbf038db3b927056106efdf91af remains unchanged. Source evidence is textual,
source_id/version null, full absolute clone path; initial offset/limit arguments
were not supported and did not bound those reads (raw output is untruncated).
[Archaeology acceptance boundary](mechanics/archaeology-mcp-acceptance.md).
No MySQL, AAPak/live client or human data is accessed by this proof.

Full validation ran sequentially at exact source470852f4 with only the named
policy/status/roadmap/scorecard/dossier/dashboard documents dirty (plus test-generated
reports later archived and restored). Production and test bytes match the source
commit. Commands:

```bash
AAEMU_LIVE_RIG=0 bash scripts/gate.sh
bash scripts/archaeology-cycle.sh
python3 -m py_compile Scripts/playertrace-coverage/dashboard_server.py
python3 -m unittest discover -s Scripts/playertrace-coverage -p test_work_dashboard.py
python3 -m unittest discover -s Scripts/playertrace-coverage -p test_quest_atlas.py
python3 -m unittest discover -s Scripts/playertrace-coverage -p test_world_geography.py
```

The full gate exits0: Release solution build succeeds, in-game compiler0errors/
0warnings,4505total/4504pass/0fail/1skip. The skipped test is
Provision_Activate_Persist_Deactivate_RoundTrip, explicitly requiring live opt-in
and isolated MySQL. Downstream BotControl44-tool and lightweight archaeology24-tool/
679-table/read-only smokes both run and pass. Archaeology cycle exits0, two Release
builds0errors (existing package warnings retained),163total/163pass/0fail/0skip,
full24-tool/679-table/read-only stdio smoke passes. Logs preserve warnings.
Dashboard syntax and44work/7atlas/5geography tests pass. Isolated loopback preview
checks both capability and milestone checkpoints at top and after scrolling,
Delivery/Sessions, tasks, coverage and atlas controls; zero page errors. Owned
preview stopped, shared8085 not reloaded. Nine generated gate reports were archived
under generated-reports before restoring only those owned test outputs. Local
documentation checkpoint follows; no fresh gate is claimed for documentation SHA.

 Artifacts live in Main's
.omp/state/artifacts/quest-stuck-recovery; canonical E152/D46 and E153 record
commands, environment, source hashes, dirty state, review and process ownership.
Linuxx64/.NET SDK10.0.110, canonical DB symlink read-only, AAEMU_LIVE_RIG=0;
AAPak intentionally unset. No Tier2/Tier3 or Login container campaign is claimed.

Single next dependency: separately allocate a bounded private-server scenario
that exercises useful nearby prey acquisition and this disclosed recovery,
including authoritative quest progress and frozen-END persistence. E149's failed
2510/3/no324 and position-conservation gap remain unresolved. No B completion,
full autonomous loop, collision-free landing, live/client/H or walking parity
is established by this slice.
