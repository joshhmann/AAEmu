# Review of OMP work through E-51

Reviewer: Astra. Date: 2026-10-07. Baseline: HEAD `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus inherited staged/unstaged/untracked work. Scope: P0.1 enrollment witnesses and E-50/E-51 review direction; no whole-project re-audit.

**Verdict: useful diagnosis; repair direction needs correction before implementation.** E-50/E-51 remain review evidence, not live enrollment proof or P0 closure. The enrollment code predates this loop as E-49 work. I cannot attribute the rest of the dirty tree to this worker from git status alone. CURRENT changed during inspection: R3 moved from CONTINUE to a recorded review closure. No new test, live lane, gameplay edit or gate was run by this review.

## What is sound

- KEEP: per-character enrollment witnesses through the existing scheduler/bridge. They improve attribution without another scheduler or rescue timer.
- KEEP: idle self-re-arm cannot create an initial wake; wire login/spawn is distinct from PlayerBot registry enrollment. The inspected field-test setup calls ConnectAsync then records START without an explicit enrollment step.
- KEEP: separate code evidence from historical live observations, refuse SQL/quest repairs from an adjacent error, and retain ordinary human login without automatic bot takeover.
- KEEP: no new live-autonomy claim from these review cards. The saved E-48 report reopens as `STALL@T+300s`, boundary `AUTONOMOUS/stall`; it does not contain the later enrollment discriminator.

## Findings, ranked

### HIGH — proposed Activate path reloads an already connected Character

E-51 proposes minimal Spawn/Activate/one-Wake for the designated networked bot. [PlayerBotManager.Activate](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotManager.cs:74) invokes `_lifecycle.ActivateHeadless(runtime.Character, botContext)`. [PlayerBotLifecycleAdapter](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotLifecycleAdapter.cs:47) calls the shared headless path and then `character.Spawn()`.

[CharacterLifecycleService.ActivateHeadless](/root/aaemu-dev/AAEmu.Game/Core/Managers/CharacterLifecycleService.cs:100) calls `EnterWorld(character, connection: null)`. [EnterWorld](/root/aaemu-dev/AAEmu.Game/Core/Managers/CharacterLifecycleService.cs:180) forces the default instance, calls `character.Load()`, sets `character.Connection = connection`, and removes owned Mates. These are fresh activation semantics, not harmless scheduler enrollment for an already embodied wire character.

Classification: MOVE OWNERSHIP / KEEP + HARDEN; SERVER ALREADY KNOWS the current Character and lifecycle state. The proposed repair must first establish whether it is provisioning a headless bot or adopting an already embodied character. Adoption needs a narrowly reviewed lifecycle/enrollment contract preserving current Character identity, connection, world state and gameplay state. Do not blindly reuse headless activation because the bridge already does it. This is a code-proved side-effect path; no new live disruption was demonstrated here.

### HIGH — E-51 chooses a new production path from a harness cleanliness constraint

[E-51](/root/aaemu-dev/.omp/state/EVIDENCE.md:1705) says only a new explicit designation path fits, because presence provisions its own bots and bridge-wake changes the no-entry predicate. This does not establish that production requires another path. A bot lacking enrollment is consistent with a test that created a normal network character without selecting a supported autonomous enrollment policy.

[The setup](/root/aaemu-dev/AAEmu.IntegrationTests/E2e/BotFieldTestV1Tests.cs:189) supplies login and natural spawn; an actual designation source/name/format remains UNKNOWN in E-51. A name assigned by the harness does not itself confer production bot status. Also, observing no-entry is a negative control: it confirms membership absence, not the intended design or repair location.

Classification: DEFER the exclusive new-path ruling; GAME-DESIGN DECISION / MISSING OBSERVATION. Compare a fresh unregistered network character with one legitimately designated bot entering through the supported production path. Disclose provisioning separately from gameplay assistance. Pre-START legitimate enrollment is compatible with unassisted post-START gameplay; no manual wake driving after START remains the acceptance boundary. A new adoption path may be warranted, but these records do not yet select it conclusively.

### MEDIUM — loop state has two active-card sections and two disposition lines

[CURRENT](/root/aaemu-dev/.omp/state/CURRENT.md:359) retains R2 as an active-card section and [R3](/root/aaemu-dev/.omp/state/CURRENT.md:378) as another, with dispositions at lines 372 and 392. Both now record STOP_DONE, with explanatory text appended. The header/table still names E-39 as the ledger maximum; later text calls this corrected without actually updating that summary. R3 also retains references to the proposed “E-51 lane” although E-51 is now a brief and the pending lane is called E-52.

Classification: REFACTOR the current summary, KEEP the append-only ledger. Keep one current card and exactly one plain disposition line. Historical closure details belong in the ledger or a clearly historical record. An old CONTINUE left anywhere would satisfy the supplied shell guard even if the newest card stopped. Do not infer the guard was used: the user's small starter command had a count only. R3 followed R2's stopping point; the visible records do not establish whether a separate assignment authorized that transition.

### MEDIUM — discriminator labels describe sampled lifecycle state, not a cause

[Disposition](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotEnrollmentSnapshot.cs:46) groups absent registration and registered-but-inactive state as `no-entry`. `executed` includes Skipped/Failed/TimedOut terminal outcomes; a future due time or lease is `due-never-executed`. These can be useful diagnostic buckets, but none means successful gameplay or a proved overdue wake.

[GetEnrollmentSnapshot](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotScheduler.cs:486) reads registry, queue, lease and outcome at separate points; one returned object is not a single atomic world/scheduler observation. Transition samples can temporarily look like a missing due entry.

Classification: KEEP + HARDEN. Preserve raw state, due time, scheduler-running flag and terminal outcome, correlate multiple samples with per-bot event/time evidence, and identify the exact failed predicate. Do not add a watchdog or claim scheduler failure from a bucket alone. The bridge emits the classification and last outcome, but not all the per-bot counters present in the record; a population TotalStepsRun delta cannot substitute for successful execution attributable to this bot.

## Next bounded direction

Stop implementation of the E-51 repair sketch pending a revised card. The highest-leverage next card records the intended supported bot designation, fresh activation versus live-character adoption semantics, and one isolated discriminator protocol containing a legitimate enrolled positive case plus the unregistered negative case. Keep no manual Wake after START, preserve natural gameplay resources, and use per-bot witnesses.

If live-character adoption is the intended product path, the card must explicitly prove no reload, connection clearing, duplicate spawn or changed Character generation. If ordinary headless enrollment is the intended path, exercise that existing owner before adding another. Client RE is not needed for this enrollment question; the server code and configured ownership policy decide it.

## Evidence boundary and checks

I read the bounded current source/diff, E-49…E-51, the relevant task-state contract/project dispatch sections, and reopened `/tmp/fieldtest-evidence/report.json`. E-49's targeted build/test totals remain inherited, not rerun. The report is a historical artifact, not a new lane observation. No independent reviewer identity/signoff was reconstructed from the ledger's ACCEPT text.

Read-only archaeology invoked via the existing Release MCP executable: `list_sources {}` succeeded; the first `read_file` attempt with path `Core/Managers/Bots/BotEnrollmentSnapshot.cs` was rejected as not found/allowed. Corrected `read_file {"path":"AAEmu.Game/Core/Managers/Bots/BotEnrollmentSnapshot.cs"}` succeeded, exact code evidence, bounds not hit, truncated false. Provenance path `/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/BotEnrollmentSnapshot.cs`, tool `read_file`, source_id/version null as returned; generation `2026-10-07T23:28:14.4236552+00:00`. Canonical DB md5 rechecked: `78b3bdbf038db3b927056106efdf91af`. These are code/data checks, not live/client/H proof. Acceptance dossier: [archaeology MCP](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).

Only this review document was written. Canonical state, worker code and lane resources were left for their current owner.
