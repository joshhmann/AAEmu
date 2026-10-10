# Review of Muse's work through E-59

Reviewed 2026-10-07, HEAD `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the existing dirty tree. Muse is still working: this is a snapshot review, not a final approval. No worker code, task state, or live server was changed. No builds/tests were rerun, to avoid competing with the active worker. Unrelated changes are not attributed to Muse merely because they appear in the diff.

**The work is moving in the right direction. The dangerous connected-player takeover was avoided. Starting a real autonomous bot still needs a live check.**

## What improved

- Muse corrected its earlier conclusion: E-52/E-53 no longer treat an unregistered test player as proof that a new bot-starting path is required. E-54 explains the difference between logging in a player and signing up a bot for AI control.
- [The activation check](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotManager.cs:73) refuses a Character with a connection before calling the headless activation code. This prevents this particular call from reloading that connected player's Character and clearing the connection.
- [The two new tests](/root/aaemu-dev/AAEmu.UnitTests/Game/Core/Managers/Bots/PlayerBotSchedulerTests.cs:1014) check refusal before the lifecycle call and successful activation with a null connection. E-57 records 36/36 scheduler tests and four consumer groups passing. Those results are recorded by Muse, not independently rerun here.
- [The action retry change](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:4874) checks the remembered finished action before the busy check. [Finish](/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/GameplayActor.cs:4950) already avoids overwriting the original result for a duplicate refusal. The added tests cover a completed movement, a retry while another movement is running, and another retry after it finishes. This is a useful targeted correction. Its new test results are not yet recorded through E-59.

## What still needs attention

**HIGH — protecting a logged-in player does not prove a bot starts and keeps working.** E-58 calls the enrollment question settled as headless-only. That resolves the chosen policy and this guard, but the live starting behavior remains unproved by the new work. The field test still creates a network-connected player; it has not been converted into a live check of a legitimately configured autonomous bot. Keep this distinction in the handoff: player protection implemented/tested at the recorded unit-test level; autonomous startup and continued activity still need live evidence. It is reasonable to work on another independent bug while that proof is pending, but do not close the broader startup outcome by implication.

**MEDIUM — non-null connection means more than an actual player connection in this repository.** [The housing test helper](/root/aaemu-dev/AAEmu.Game/Models/Game/Bots/BotDriveBridge.cs:3468) attaches a `GameConnection(new E2eNullSession())` to a headless character. The new guard refuses that too if the character later needs activation. It is a safe conservative refusal, but it can affect an existing test sequence. E-57's consumer tests do not establish compatibility with that housing sequence or the wire-connected quest/farm wake helpers. Check those uses and explicitly record which now refuse by design. Do not weaken protection or hide the change to preserve a test pass.

**MEDIUM — the proposed next card groups several separate problems.** E-59 combines finished-action retries, old Character versions after restart/reactivation, and what happens after a failed bot step. These should be separate reviewable changes. The currently visible retry edit is small and directly relevant; finish its focused verification first. Then give the failed-step problem or stale-Character problem its own outcome and evidence. No second scheduler, guessed retry timer, or large replacement framework is needed to repair the finished-action case.

The guard does not by itself establish that a currently AI-controlled character cannot later acquire a real connection. Normal managed bot account policy may prevent that, but this review did not prove every login/reconnect case. Keep the narrower claim: this Activate call refuses a connected Character.

## Next useful checks

1. Record the focused retry test result on the actual final source state. Retain the completed result through busy and later retries; the refused duplicate must never start again.
2. Check the existing housing and connected quest/farm test helpers affected by the guard. Report intentional refusal separately from accidental breakage.
3. Run one assigned isolated live check using the existing supported bot setup: AI starts, multiple turns belong to that bot, and it continues without manual wakes after START. Disclose setup gifts and do not claim self-acquisition from provisioned items.

This review does not authorize a lane launch, deployment, or repair beyond the user's current assignment. The source changes are still uncommitted. Full gates, live behavior and human playability are separate pending checks.

## Evidence inspected

Read E-52…E-59, the relevant manager/scheduler/actor/test diff, adjacent bridge callers, and the existing headless provisioning comments. Source line numbers describe this moving snapshot. E-56/E-57 attribute authorization to the user; I did not reconstruct the separate worker conversation or claim independent signoff.

Read-only archaeology: existing Release MCP executable, `list_sources {}` and `read_file {"path":"AAEmu.Game/Core/Managers/Bots/PlayerBotManager.cs"}` both succeeded. Returned path `/root/aaemu-dev/AAEmu.Game/Core/Managers/Bots/PlayerBotManager.cs`, source_id/version null as returned, generated_at `2026-10-08T06:55:02.6450051+00:00`; truncated false, bounds not hit. Confidence exact for inspected code, not live/client/H. Canonical DB md5 rechecked `78b3bdbf038db3b927056106efdf91af`. See [archaeology acceptance dossier](/root/aaemu-dev/scorecard-explorations/mechanics/archaeology-mcp-acceptance.md). No gameplay gate claim follows from these reads.
