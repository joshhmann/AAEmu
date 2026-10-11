# Nearby quest prey selection — 2026-10-11

Parent: approved supported quest autonomy, existing-owner progression.
This local slice repairs destination selection and implements bounded perception
for the existing NPC-loot quest objective family. It does not complete that track.

## Before and after

Before, a bot without nearby quest prey walked toward the first matching spawner.
Reversing two matching sources changed its destination from 80m to 1400m. The
ordinary NPC observation contained only targets within 25m, so a living quest mob
55m away could not be selected. E150 reproduces both gaps through seeded production
owner/arbitration/quest/travel paths before production source edits.

After, actors with active quests expose a separate living-NPC census within 100m
through the existing region query. The boundary is exclusive, matching that query.
The immutable decision snapshot copies it. The existing NPC-loot quest selector
ranks the distinct union with the ordinary 25m census, retaining quest relevance,
liveness, hostility, CanAttack and CanSeeTarget, valid committed-target preference,
sibling contention and the stale-target recovery policy. An uncommitted solo bot
chooses the nearest suitable prey; it does not switch away from useful committed work.
The travel guard and NPC-loot source signal for deferred-owner recovery use the
same suitability checks. If none is suitable, both the Advance gate and ordinary
quest travel use the nearest finite matching source in the current world's spawner
collection. Equal distances use coordinates and spawner ID, independent of load order.
A known spawner is a search location, never proof that a mob is alive or a route works.

## Ownership, scope and non-goals

Main implements/integrates and owns acceptance. same_npc_discovery supplies bounded
independent textual/MCP source review; it does not execute builds or certify live play.
Source/test scope: ActorObservation.cs, BotDecisionProposal.cs, GameplayActor.cs,
QuestObjectiveTargetSelector.cs, QuestBehavior.cs, BotRoamStepExecutor.cs,
BotOpportunityListing.cs and QuestOwnerHandoffTests.cs under the existing bot paths.
Two existing test fixtures, HeldoutRecoveryTests and LoopRedispatchTests, now place
prey beyond100m when testing unseen-source travel and stale-loop redispatch.
No new manager, scheduler, strategic selector or gameplay path. Character/services
still own movement, attackability, combat, loot rights, prerequisites and quest credit.
No client/server data edits, NPC relocation, injected drops/rewards/credit/prerequisites,
quest assignment, synthetic live mobs, forced save, global spawn-loader rewrite,
reporter lookup change or admin feature. Existing 25m interactions, giver discovery,
corpse census and loot safety remain separate. This wider band is consumed by the
existing NPC-loot objective selector, not every hunt/talk/objective family.

Local branch fix/quest-prey-source-ranking-2026-10-11 builds on clean unmerged
c23aee4750099ced1b82ba11c8c41fabf8073ddf corpse checkpoint. Main develop remains
2f93a2fc998704835c856b68ebefaf406b9c2840. No merge/push/shared deployment or new
live run: D42's budget ended with E149. Historical E139 pass and E142/E149 failures
are preserved. Vibe OFF; no Muse, human/H, grade, milestone or seven-criterion promotion.

## Risks and acceptance

The scan is bounded spatially and only runs with active quests; no load/scale claim.
It uses horizontal distance and the server region query, not newly implemented LOS,
path-cost or reachability ranking. Existing travel owns path failures and retry bounds.
Finite coordinates can overflow float distance, so nonfinite computed source distance
is also excluded. Observation IDs are copied, not retained entity references; current
NPC liveness/legality is rechecked during choice and ordinary execution.

Controls cover closest distinct living NPCs at 55/70m before remote-spawn travel;
nearest 80m source despite far-first/reversed order and an interrupted request under
the same retained owner; dead/hidden/wrong-template/nonhostile/out-of-band prey cannot
suppress a valid fallback; 99m admitted, 100/101m excluded, copied observation and
post-capture target movement; committed valid prey retained then reselected after
fixture death; source ties, wrong templates, invalid coordinates, infinite computed
distance, missing/other-world sources; unchanged reporter lookup; deferred quest
readmission at both 20m and 55m. Existing same-NPC B selection, ineligible offers,
finite exhaustion, missing-source and hybrid-doodad cases remain in the same suite.

All fixture quests, NPCs/spawners, regions and loot-chain rows are explicitly seeded.
Acceptance/selection/requests run through ordinary production code; death and source
availability changes in controls are fixture manipulation. These tests prove choice,
ownership and refusal, not real spawning, kills, loot generation, completion or navigation.
Initial second NPC setup mistakenly reused HeadlessSession.SpawnNpc's template-deduped
object; that intermediate 9/10 result is retained. The corrected distinct-candidate
fixture uses the established extra-NPC pattern. A later 17/19 run exposed two fixture
assumptions (unseeded ZoneManager during friendly assertion; exclusive radius boundary).
Both were corrected without changing authoritative gameplay or scan radius.

## Evidence and required validation

E150/D44 qualified implementation. E151/D45 accept the final local source/rig
outcome within this bounded family. Before: dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj
--configuration Release --treenode-filter '/*/*/QuestOwnerHandoffTests/*', c23 plus
one test file dirty, 10total8pass2fail0skip. Initial failure at 55m is valid even though
the first two-candidate fixture reused one object. Corrected 19 controls passed before
final computed-distance refusal was added; all19 then passed after that change.
First full gate at16fb built successfully, compiler0errors/0warnings,4468pass/
2fail/1skip. Both failures used the previous25m census boundary: fallback26m and
committed target60m. They now place unseen prey101m away and retain their original
travel/redispatch assertions; the fallback also asserts the wider census excludes it.
The next full-suite invocation reported4469pass/1fail/1skip: the101m source
requires an existing60m first travel leg at the fixture speed. The assertion now
checks that bounded first leg while retaining the final101m route destination and
movement/owner checks. No travel constants or production files were changed.
The first combined-filter invocation selected the full suite; its actual count is
recorded, without presenting it as a small focused run.
Required: focused 19 controls, independent final source review, AAEMU_LIVE_RIG=0 bash
scripts/gate.sh and bash scripts/archaeology-cycle.sh sequentially, plus bounded UI
checkpoint rendering verification. Tier3, Login CI-parity, fresh live/client/H are not run.
Artifacts and canonical ledger: Main .omp/state/artifacts/quest-prey-source-ranking/.

Environment Linux x64 SDK10.0.110; read-only canonical compact.sqlite3 1.2 r208022,
MD5 78b3bdbf038db3b927056106efdf91af. No MySQL/game_pak needed for source gates.
Archaeology 24-tool catalog, 679 tables; exact NPC3475 level2 and bounded configured
spawn data, source/code textual evidence. Catalog/schema/query/trace/read_file inputs
and full outputs preserve source_id/path/version/confidence/truncation. Initial SQL
used absent loot_pack_id, failed, and was corrected after schema read; no loot claim
was inferred. Main initial source2f93 and owned clonec23 relevant source agree before
repair. Read-only source snippets may be truncated in reviewer reads as recorded.
See [archaeology acceptance](mechanics/archaeology-mcp-acceptance.md).

ACore is a pattern reference only: mod-playerbots/mod-playerbots source pin
79bd428115c8f33b74b13de23b133d49c3f75c60, PossibleTargetsValue, GrindTargetValue,
ChooseTravelTargetAction and TravelMgr. Its configured nearby scan/ordinary quest-loot
checks and nearest solo selection informed this shape; its travel is not universally
nearest and no WoW eligibility/data or recovery shortcut was copied.

## Remaining gap and single next dependency

A fresh bounded private server validation of the repaired source must show useful
prey acquisition, real kills/loot and ordinary completion/independent follow-up.
No live run is allocated here. E149 quest2510/3, no324 and strict frozen-END XYZ
conservation failure remain unproved; this source repair does not erase those outcomes.

## Final receipt — E151 / D45

Production commit `16fb3c2ccdfb00d724e62e532dc049532446883c`; two test-only
follow-ups `376737c29dcc452cb8575b7595c6d90b864cea99` and
`7cc7188d06cfd7693abbc374d63a92c7b75f6d8a`. Main develop remains
`2f93a2fc998704835c856b68ebefaf406b9c2840` clean. Full final gate and archaeology
ran at7cc7188d with only pending records/dashboard and test-generated reports dirty;
no production dirt. Documentation checkpoint committed afterward; production
hashes remain identical and no new full gate is claimed at that docs SHA.

Commands in owned normal clone `/root/aaemu-overnight-quest-20261011`:

```bash
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --treenode-filter '/*/*/QuestOwnerHandoffTests/*'
dotnet test --project AAEmu.UnitTests/AAEmu.UnitTests.csproj --configuration Release --treenode-filter '/*/*/HeldoutRecoveryTests/*'
AAEMU_LIVE_RIG=0 bash scripts/gate.sh
bash scripts/archaeology-cycle.sh
python3 -Werror -m py_compile Scripts/playertrace-coverage/dashboard_server.py
bash Scripts/playertrace-coverage/regen_static_dashboard.sh
```

Focused19pass/0fail/0skip; held-out5pass/0fail/0skip. Final Release solution build
successful; in-game compiler0errors/0warnings. Full unit4471total/4470pass/0fail/
1skip: `Provision_Activate_Persist_Deactivate_RoundTrip` requires explicit live rig
and MySQL configuration and was not exercised. Downstream BotControl44-tool stdio
and lightweight archaeology24-tool/679-table read-only smoke both ran successfully.
Full archaeologycycle: Release MCP and unit builds0errors,163focusedpass/0fail/
0skip, full24-tool read-only smoke passed; AAPak intentionally unconfigured.

Independent reviewer checked all7 production hashes and both fixture adjustments;
textual/MCP review, not test/live certification. Final checkpoint MCP at7cc7188d
re-read complete selector and QuestBehavior source, untruncated textual evidence;
exact bounded NPC3475 query returnslevel2 with compact1.2r208022 provenance.
Canonical DB MD5 unchanged. Raw tool inputs/output preserve all metadata.

Dashboard work44/atlas7/geography5 Python tests passed. Existing capabilities and
milestones paragraphs and generated HTML updated together. Owned localhost48586
browser preview checks both top/scrolled panes plus work/tasks/coverage and atlas
controls; screenshots and no-page-error receipt retained. Preview cleaned up,
shared8085 process untouched. All seeded/bypassed steps and evidence limits above
remain; no new game processes or MySQL mutation, live/client/H, Tier2/Tier3, Login
CI-parity, merge, push or deployment. Test-generated reports archived then restored
only in owned clone. Canonical E-ledger and dashboard preserve predecessor records.
