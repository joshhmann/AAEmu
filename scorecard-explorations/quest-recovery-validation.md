# One fresh quest recovery validation — E167

The bounded live validation passed. The bot independently accepted quest251, recovered after one controlled movement interruption and a genuinely failed resumed chase, collected all three required items, turned in251 and independently accepted324. Its exact saved body/position matched the stopped END both after release and after325seconds. This is a supported251→324 acceptance witness, not universal quest autonomy or human/client acceptance. Quest324 completion was not exercised.

## What the bot did

Character1/Qproofoc, actor44103, enrollmentgeneration1. Ordinary quest completions were330,6198,2531,251;12request-local work witnesses. Level10starter/equipment/onewake were supplied setup, not earned. Quest/target selection was not externally assigned.

| UTC event | Authoritative receipt |
| --- | --- |
| 22:13:39.3989714 | Independently accepted251 at NPC3512 |
| 22:13:50.3000193 | Victim449ce69f-464d-4265-97b9-20a2b8b9b6f7 Interrupted, target37250 |
| 22:13:51 control observation | Interrupt709bbce5-accb-452b-9796-98b7b233b955 Completed/result_payloadtrue |
| 22:14:04.5996523 | First freshad4ba083-cd08-4cb1-a57c-5938a9dce308 TimedOut/Navigation; failure retained |
| 22:14:16.116447 | Distinct fresh02a1c557-79b8-4099-bb9a-c8d42978fea2 Completed |
| 22:17:58.3811433 | Lootb42172e1-f277-4890-95b1-9df2df15f225 target44318:4058quantity/objective0→1 |
| 22:19:30.772259 | Lootcd5cdf62-8523-4d02-966c-0b9de3ec8a15 target37255:1→2 |
| 22:21:45.4083248 | Lootcefb050b-2634-49a0-ab2c-a4db18d63624 target37258:2→3 |
| 22:22:19.8585204 | TurnIncab2bda9-a289-48d7-992b-18a59363491f completes251 |
| 22:22:35.7761916 | Accepta1787376-03db-487b-b755-4277a622e858 independently selects324 at NPC3513 |
| 22:22:36 | Private schedulerStopped, exact ordinaryStop terminal, fresh full END, release/sessiondeactivation/absence |
| After325s | Full body/XYZ matches, releaseProved/flushProvedtrue, no differences |

The revised E165consumer records exactly one recovery: control709bbce5/victim449ce69f/response02a1c557/consequenceb42172e1. It does not credit the timed-out ad4ba083 move. The first subsequent response completed; root loot supplies the actual quest effect. Persisted request history contains2357 distinct terminalIDs and no changed repeated terminal rows. This corroborates exact retirement/reselection in this case; it is not exhaustive proof of every dispatch or retry. Eligibility/retention/give-up/exhaustion negatives remain E165/E136 scoped local/historical evidence. Fresh same-NPC pair was not encountered:251/324differentNPCs; E1572255→2256sameNPC evidence remains historical.

Early claim-unresolved logs showed no pinned corpse, not the ownership or contents of the two dead boars. Read-only review corroborated actual damage and kills, later ordinary loot, existing dead-target/perceived-corpse pinning and empty-container exclusion. Which condition excluded the first two corpses remains unobserved. No production defect was established, so no code change was manufactured. loot-claim-review.md has source/data provenance and limits. No QUEST_STUCK_RECOVERY/teleport witness captured; relocation not credited, no universal walking claim.

## Validation and ownership

Overall live1total/1pass/0fail/0skip/rc0,17m11s238ms. PRODUCTIVE_QUEST_COMPLETE at701.7958458autonomous seconds;325sflush. EvidenceFault/latestCopyFaultnull/failuresempty; rawPersisted/proofAcceptedtrue. Ordinary registry removal/sessionrelease/absencetrue, exactlyonewake/oneinterrupt. END actor44103/gen1/schedulerstopped/backgroundidle/autoattackfalse, ordinaryStopterminal; no fault clearing/substituteEND/position tolerance relaxation.

Command in owned normalclone: private0600runtime-env with BOT_PRODUCTIVE_RUN=1 plus timeout --signal=TERM1700 dotnet AAEmu.IntegrationTests/bin/Release/net10.0/AAEmu.IntegrationTests.dll --filter-method AAEmu.IntegrationTests.E2e.BotShortRunTests.ProductiveQuestHeadless15Min --report-xunit-trx --report-xunit-trx-filename quest-recovery-revalidation.trx --results-directory $E2E_ROOT/logs/test-results. Integrationbuild0errors; timeout420 established e2e-boot --clone-data-from /root/aaemu-e2e passed. Fixture360/observe900/noactualprogress300/operationroom5/poll1/flush325/logcaps unchanged. Actual boot allowance420 is authoritative, not historical360 reporting field. No additional test/retry/allocation.

Linuxx64 SDK10.0.110/runtime10.0.10/MySQL8.0.36privatecompose. Root /root/aaemu-e2e-recoveryproof-20261011/projectrecoveryproof20261011, private0700root/0600env/config and freshauth/DBpassword. Existing24885257728bytepak/canonicaldata reused; no downloads. Main humanclient/launcher missing and not required for headless server evidence; H remains UNKNOWN. AAEmu setup skill used with established E2E lifecycle. Initial absent historical private capguard corrected to standard repository fallback before any server/test started; sameallocation. Config permission tightening changes metadata only, no run settings.

Main develop HEAD2f93a2fc998704835c856b68ebefaf406b9c2840 plus7dirty docs/UI preserved. Tested candidate fix/quest-useful-choice-2026-10-11 HEAD1da6ede32146ecfe234063c3329be47be97f4ce3 plus19dirtypaths. Allnine sourceSHA256s fixed and unchanged before/after; source was not edited duringrun. This new dossier adds a twentieth documentation path after testing. Candidate has not been committed/merged/pushed/deployed. E130 merge remains E145/D40.

E165 historical unchanged-source local checks retained:240focused/fullgate4514total4513pass0fail1skip Provision_Activate_Persist_Deactivate_RoundTrip/compiler0errors0warnings/BotControl44+lightarch24, fullarch163/163/full24smoke. Those gates were not rerun as fresh checks duringthisvalidation. Catalog-first current readonly archaeology list_sources/list_tables679/trace_quest251/find_quest_objectives251limit12 allok; MD5 78b3bdbf038db3b927056106efdf91af/runtimecanonicalmatch/version1.2r208022. Toolinputs/provenance/source_id/path/version/generated_at/exactdata versus heuristicobjectivechains/truncation in mcp-input/output/provenance; code excerpts/review textual, partialwindows preserved. See scorecard-explorations/mechanics/archaeology-mcp-acceptance.md. No AAPak tool configuration claim.

Cleanup completed: Login970361/Game971825/monitor964538 absent, exact projectcontainer/volume/network removed, sevenportsfree, fiveforeignPIDs alive/preserved. Game required established bounded force-stop after accepted flush; gracefulGame shutdown is not proved. Private0700history/0600env/config retained, no active owned runtime/check/worker. Dashboard controls did not launch/stop actual processes. Main retains architectural/acceptance verdict; independent corpse-review worker read-only, no source/build/runtime/control operations.

## Capability and remaining boundary

Before: E163 ordinary251progress/nextchoice and strictEND were real, but its controlled-recovery consumer rejected the first resumed timeout; overallFAILED. E165 repaired and proved bounded consumer handling locally/replay. After: a fresh single private run now passes controlled recovery plus real251completion/independent324accept and strictEND/release325s. Production gameplay is unchanged in this validation. E163 FAILED is preserved rather than retroactively turned green.

Seeded/bypassed: level10starter/equipment/onewake only; no assigned quest/target/route, injected prerequisites/questcredit/items/rewards, forceddeath/kill/loot/save/restart or teleport.324ordinaryacceptance supplies its questitems through the engine; no harvesting/Bcompletion claim. No repeatability, universal/generalfull-loop, freshsameNPC/ineligibleoffer, danger/IDcounterfactual, client/H, grade/parent/seven-criteria promotion by implication.

Single next dependency: review the owned tested candidate and inherited develop changes for a clean integration checkpoint before publication or broader gameplay work. No automatic merge/push/deploy follows this successful child-slice result. Artifacts and exact final receipt: .omp/state/artifacts/quest-recovery-validation-20261011/{final-receipt.json,actual-chain.json,live-report.json,live-run/observations.jsonl,live-result.json,runtime-identity.json,cleanup-receipt.json,loot-claim-review.md,source-hashes.json,baseline.json,contract.md,boot.log,supervisor.log,compose-down.log,mcp-*,board-*}.
