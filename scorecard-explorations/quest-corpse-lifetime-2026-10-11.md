# Fresh quest corpse recognition — bounded implementation

Baseline: develop `2f93a2fc998704835c856b68ebefaf406b9c2840`. Work is isolated on
`fix/quest-corpse-lifetime-2026-10-11` in `/root/aaemu-overnight-quest-20261011`.
Main owns architectural and acceptance verdict. Local commits only; no merge,
push or shared deployment. E147/D42; predecessor E142 remains failed evidence.

A bot successfully loots a boar, then a newly spawned boar can reuse its object
number. The existing quest loot memory, terminal ledger and request key treated
that number as the same corpse. Two seeded ordinary-decision tests reproduce
the exclusion before repair: 6 total,4 passed,2 failed,0 skipped. Both observed
alive and first-seen-dead replacements failed after a successful real bag grant.
This proves a code blocker; E142's actual third-container contents remain unknown.

The repair binds existing memory and proposals to the actual NPC instance using
a weak reference token. A replacement gets a fresh request key; the same NPC,
even with a refilled container, remains spent. Prepared take/skip publication
checks the captured character/world/NPC. The existing GameplayActor Loot path
checks the binding and exact resolved owner before ordinary OpenBag. Stale
requests receive ordinary audited rejection. No new quest/combat/loot behavior
service, rewards, objective credit or prerequisites are introduced.

Owned files: QuestBehavior, LootBrainPlanner, LootLedger, new LootCorpseIdentity,
GameplayActor, QuestCorpseRepinTests, GameplayActorLootGrantTests, this dossier
and current STATUS/SCORECARD/ROADMAP notes. No shared-core NPC/spawn change.

Acceptance: replacement grants once through ordinary decision/authoritative Loot;
same-instance refill grants nothing; stale prepared take/skip and another actor
cannot publish a take; ordinary generation retains active quest, absent quest,
zero rate and independent optional-pack controls. Existing interruption, claim,
empty, world and bounded travel controls must remain green. Full standard gate
and archaeology cycle required before the one bounded private live validation.

Risks: instance identity is process-local and not persisted or sent to clients.
Existing bounded cache full-clear policies remain; this does not establish
unlimited-history guarantees. Request binding handles replacement at resolution;
ordinary server execution/thread ownership still applies. Runtime loot mirrors
exercise the real generator but do not prove full NPC death or loader behavior.

Evidence layers: seeded active251/NPC/container/recycled-number rig checks real
decision and bag grants, without proving live quest eligibility or full death.
Generation controls use ordinary canonical251 acceptance at level2, with explicitly
seeded canonical pack rows and synthetic corpse template. Alive-wake rig death
sets HP only and explicitly tears down its attack through public StopAutoAttack.
The planned private run uses level10 ordinary starter setup, one enrollment wake,
passive observation and one exact owned interruption; no assigned quest/route or
external kill/loot/credit. Its outcome is pending. No B completion, full loop, H,
seven autonomy criteria or parent milestone promotion follows from rig proof.

Read-only archaeology: catalog/schema/bounded SQL at baseline HEAD, canonical
compact.sqlite3 1.2r208022,MD5 `78b3bdbf038db3b927056106efdf91af`;679tables.
Exact NPC3475 pack4530/item4058 quest251 and optional8055/group829 facts, no
truncation. Source relationships are textual; live behavior unproved. Tool inputs,
raw provenance and logs: Main `.omp/state/artifacts/third-boar-loot/initial-mcp-*`,
`loot-generation-review/`, `corpse-selection-review/`. See the
[archaeology acceptance dossier](mechanics/archaeology-mcp-acceptance.md).

E142 END forensics: its observe cancellation aligns with the declared no-progress
deadline; frozen END had an active Move before release. No save bug is established.
No recopy, movement tolerance or persistence oracle change is part of this repair.
Final checks, exact SHA/dirty identity and live receipt will be recorded below.

## Source validation — E148

Reviewed local source commit `3cf0ebbd29dc47c9b426fe25a4b284b0d39a1993`.
Main ran `AAEMU_LIVE_RIG=0 bash scripts/gate.sh`: Release build succeeded,
ScriptCompiler0errors/0warnings;4460total,4459passed,0failed,1skipped
`Provision_Activate_Persist_Deactivate_RoundTrip`. BotControl44 tools and light
archaeology24tools/679tables/read-only smoke passed. `bash scripts/archaeology-cycle.sh`:
both Release builds0errors;163/163passed,0fail/skip;full24tool readonly smoke passed.
Integration DLL filter classes BotShortRunEvidenceTests/ProductiveQuestEvidenceTests/
QuestContinuationEvidenceTests:125/25/25passed,0fail/skip. Linuxx64 SDK10.0.110,
canonical MD5 unchanged; no MySQL/live/AAPak in these checks.

Nine focused corpse controls pass and all four generation controls pass in the
full unit suite. The first-seen-dead failing-before case isolates the reused-number
blocker. The observed-alive fixture also needed public StopAutoAttack after its
HP-only synthetic death; its initial RED is not an isolated proof of that blocker.
This setup correction and intermediate failed guard/test-constructor attempts are
retained in raw logs; full gate uses the final committed source.

Source review was independent textual inspection, with byte-bounded/truncated
MCP source reads; Main exercised tests. Reviewed hashes match all five production
files. Final MCP SQL at3cf0ebbd is exact/untruncated; source provenance has explicit
path but null source_id/version, and reads are intentionally truncated.

At gate launch dirty state was3modified current documents plus1new dossier; source
committed. Nine owned gate-generated reports were archived with SHA256 and restored
to HEAD afterward. Current private boot/runtime/source/test hashes are retained
in Main artifacts/third-boar-loot/; source remains frozen during the live check.

## Single private validation — E149

Source and test binaries were frozen at3cf0ebbd; dirty state3currentdocs+1dossier.
`timeout420 bash Scripts/e2e/e2e-boot.sh --clone-data-from /root/aaemu-e2e` booted
projectcorpselife20261011 on seven private ports43511/30514/30517/30519/30550/
30580/30560. Existing external1.2pak/compact were reused; MD5 unchanged.
Private600 runtime-env with BOT_CONTINUATION_RUN=1 ran `timeout1700 dotnet
AAEmu.IntegrationTests/bin/Release/net10.0/AAEmu.IntegrationTests.dll
--filter-method AAEmu.IntegrationTests.E2e.BotShortRunTests.QuestContinuationHeadless15Min
--report-xunit-trx --report-xunit-trx-filename corpse-continuation.trx
--results-directory $E2E_ROOT/logs/test-results`. Tier2live1total0pass1fail0skip,exit2.
No full compiler/unit/MCP pass is implied by this live command; those are E148.

NOT_PROVED: observation ended NO_PROVED_PROGRESS at465.8459525s,300.0000235s
after independent251acceptance at165.845929s. Ordinary330/6198/2531 completed
before251. Quest251 stayed0/3 with no Loot, and324 was not reached. The bot
selected3475source(14466,14410.39),1409.8m fromgiver; it did not arrive before
the no-progress deadline. No live corpse replacement or productive recovery was
exercised. One exact owned251Move interruption was acknowledged and fresh251
travel resumed; the report's productive Recoveries array is empty.

The existing frozen-END oracle failed after release and after325sordinaryflush.
Only reported full-character differences are X/Y/Z,consistent with approximately
1.96m of ordinary travel between copiedEND and removal. This does not establish
a save bug. No snapshot recopy, tolerance, forcedsave or unrelated repair was made.
Registryremove/sessionrelease/registryabsence all true; waitSeconds325.
Owned Login3410175/Game3412140 stopped,all7ports free,owncontainer/network/volume
removed. All six inventoried foreign processes remain alive; Main develop remains
clean at2f93a2fc. Runtime/test/source hashes and full raw artifacts retained under
Main `.omp/state/artifacts/third-boar-loot/live-run/`, source-freeze/runtime-identity,
cleanup-receipt and final-receipt. No second run or shared deployment.

## Single next dependency

Reproduce and repair order-sensitive quest prey source selection in its own
bounded slice: `TryNpcSpawnerPosition` takes the first matching spawner, without
distance ranking. Identical old/new NPC spawn files currently have opposite
directory enumeration order; SpawnManager reverses that unsorted order. This
predicts current remote wildlife source(14466,14410.39) versus the base local
source(15586.546,15117.345). Historical .NET boot enumeration was not logged, so
that causal link is strongly supported, not a directly observed boot-order fact.
Exact hashes/rows/ordered filenames/bounded MCP source provenance are retained
in spawn-source-review/. Establish nearest valid same-world prey selection and
order/eligibility controls before choosing another repair or requesting live work.
The current corpse repair is source/rig accepted and locally committed; broad
251/324continuation/live corpse acceptance remains unproved. Earlier E139 passing
evidence and E142 failed evidence remain historical. No H/client, full loop,
seven autonomy criteria,grade or parent milestone promotion.
