# Individual PlayerBot activity choice and human invitations — delivery specification

- Audience: Project owner, contributors, implementing agents, and independent reviewers
- Last verified against: source/documentation inspection on 2026-10-16 at `develop` / `26612d5902ff24d860bbf99be7dbcb3f5c3b76e8`, with inherited dirty work preserved; no new gameplay or client verification
- Status: Parent specification awaiting bounded ticket decomposition; not an implementation allocation or capability verdict
- Tracker: Delivery & Sessions, parent card `playerbot-individual-activity-choice`, under Shared autonomy

## Problem statement

Players should meet individual bots participating in the shared ArcheAge world, not permanent occupational agents confined to one settlement. A bot should have reasons to farm, build a homestead, craft, carry a trade pack, fight, grind, or join other players. Its interests, resources, and the market should influence those choices without turning every bot into the same profit-maximizing worker.

The system can supply activity know-how. Requiring a bot to discover the existence or procedure of every activity before it can participate is not the desired product. Conversely, supplying know-how must not fabricate the resources, eligibility, travel, or successful consequences of doing that activity.

Choices also need continuity. A more attractive opportunity should not make a bot abandon a delivery or switch tasks every decision tick. Human invitations need understandable responses, including refusal or a decision to finish existing work first. Silence is not an acceptable substitute for a decision.

AAEmu already has ordinary Characters, activity arbitration, actor requests, domain policies, personality and resource inputs, party actions, and scenario/test facilities. This specification connects and extends those existing capabilities; their presence is not evidence that the complete requested behavior is already implemented or verified.

## Solution

Let each bot choose among supported activities using supplied know-how, individual motivations, actual personal resources, and relevant market conditions. Execute its choice through ordinary gameplay, retain that commitment until an activity-appropriate boundary, and then reconsider fresh opportunities. Allow a bot to accept or decline a human invitation while ensuring that the player receives an explicit response.

This applies across the shared world. Regions differ in race eligibility, items, resources, and local content while sharing recurring kinds of activities. A bounded regional demonstration supplies evidence, not a settlement-sized product boundary or a claim that every regional variant works.

Retain the existing supported-capability contract: legal actions, observed consequences, bounded recovery, and subsequent independent choice. A bot need not earn a complete player lifetime before a useful supported activity can be verified. Supplied starting prerequisites remain disclosed setup, not earned acquisition.

## User stories

1. As a player, I want bots to participate across the shared world using locally appropriate content, so that one town or demonstration route does not define their entire world.
2. As a player, I want an individual bot to choose different activities rather than hold an assigned permanent occupation, so that the same character can develop varied interests and routines.
3. As a bot designer, I want to supply supported activity procedures and available kinds of work, so that bots can participate without first discovering how the game works.
4. As a player, I want bots to obey ordinary eligibility, ownership, costs, timing, and reward rules, so that supplied knowledge does not create unfair or imaginary outcomes.
5. As a player, I want bots with a dominant motivation or a weighted mix of motivations, so that different individuals can prefer different activities in the same circumstances.
6. As a bot, I want my finances, supplies, and relevant market conditions to influence my choice, so that I can respond to affordability, shortages, and economic opportunities.
7. As a player, I want a sufficiently resourced bot to pursue a preferred activity even when another activity is more profitable, so that personality matters rather than every bot making the same financial choice.
8. As a player, I want a bot to follow through on its chosen activity rather than repeatedly switch to a higher-scoring opportunity, so that its behavior is coherent and work reaches an actual outcome.
9. As a player inviting a bot carrying a trade pack, I want it to finish delivery before joining my party if it chooses to join, so that my invitation does not discard its existing commitment.
10. As a player inviting a grinding bot, I want it to be able to finish its current fight and then join, so that I need not wait for its entire grinding session.
11. As a player, I want an explicit response when a bot accepts, declines, or needs to finish current work before joining, so that I can understand its decision rather than encounter silence.
12. As a reviewer, I want choices, completed work, invitations, and follow-up behavior checked against actual bot and world outcomes, so that plans, messages, and fixture setup are not mistaken for completed gameplay.

## Implementation decisions

### Scope, knowledge, and regional facts

Extend the existing PlayerBot activity policies and opportunity providers. The system supplies supported activity/action know-how and loops; there is no requirement for a learning or knowledge-discovery simulation. At choice time the bot must still establish valid targets, ordinary eligibility, location, access, resources, and current availability. Knowing how to deliver a trade pack does not establish ownership of a pack or successful delivery.

Treat the existing autonomy criterion of discovering a valid gameplay opportunity as establishing a real eligible opportunity, not independently learning the existence or procedure of an activity. Do not remove the criterion, replace it with a configured activity name, or claim discovery when only know-how was supplied.

Declare supported activities and regional variants explicitly. Reuse shared gameplay across regions and verify the local differences. Do not duplicate a global defect per region, require an exhaustive world census before the first useful slice, or silently generalize one region's result to the world. Homesteading, farming, crafting, trade-pack runs, PvP, PvE, and grinding are product activity families, not an assertion that all variants are presently available or proved.

### Individual motivations and economic influence

Keep motivations separate from occupations, immediate needs, and changing opportunities. A bot may have one dominant player type or a weighted mix. Bartle's taxonomy is possible vocabulary, not a selected mandatory enum, exact dimension set, scoring formula, or dependency on a new personality framework.

Reuse existing personality/profile and preference inputs where they support this meaning. Reconcile their actual consumers before extending them. Do not turn an existing farmer/trader profile label into a permanent restriction on what its Character can do.

Use actual personal finances and supplies alongside relevant market information when choosing among eligible activities. Legal and affordable participation constrains choice before preference ranking; preference does not authorize missing materials or unpaid costs. Profit is one influence, not the sole objective. With adequate resources, an individual can select preferred work despite a more profitable alternative.

The exact motivation weights, market signals and freshness rules, and comparative economic thresholds are not settled by the interview. Ground them in existing consumers and ordinary service data during ticket decomposition. Do not assume omniscient market knowledge, invent unavailable prices, or introduce a replacement economy to fill those gaps. A ticket whose acceptance depends on a material unresolved policy is not Ready.

### One existing owner for choice and execution

Reuse the ordinary Character lifecycle, PlayerBot scheduler, activity arbitration, domain goal owners, GameplayActor/ActorRequest path, and existing gameplay services. Existing domain planning or GOAP remains where it is actually wired; this does not mandate a universal GOAP conversion, another decision loop, or a second scheduler.

Activity choice requests work; the ordinary action result and authoritative world state establish its consequence. Completion, failure, cancellation, and ownership release must agree before another activity takes over. Do not treat a predicted effect, accepted request, success-sounding message, or synthetic completion callback as a finished activity.

Use existing Character/profile/metadata persistence only for justified durable state. Reconcile current world state and create fresh requests after restart; do not persist and replay an in-flight ActorRequest. Any additional durable motivation or commitment data needs a bounded persistence contract in its child ticket, not an assumed schema change here.

### Commitment follows the activity

Retain a chosen activity across ordinary opportunity rescoring. A better score alone must not steal its owner or discard its work. Define commitment and the next legal interruption boundary in the responsible activity policy, using existing result and cancellation ownership.

The agreed party-invitation cases are:

- A bot carrying a trade pack completes its delivery before joining, if it chooses to join. Observe the ordinary delivery result, including the pack and applicable transaction consequences, before reporting that this commitment is satisfied.
- A grinding bot can finish its current fight before joining. It is not required to finish a whole grinding session. Observe the fight boundary rather than bypassing combat or granting a kill.

These are examples of activity-specific commitment, not a universal session length. If the activity fails or the invitation ceases to be valid, it cannot manufacture completion or join using stale eligibility. Handle that through the existing bounded failure and invitation paths. Danger, death, unavailable delivery, and other exceptional interruption priorities remain unresolved product policy; preserve existing ordinary safety/refusal behavior until a bounded decision changes it.

### Human invitations and player-visible responses

Extend the existing native party invitation/response path rather than silently accepting every human request or creating a parallel party implementation. A bot may decline based on its choice. A response must reach the inviting player; an internal decision or server log alone is insufficient.

Separate the bot's communicated intention from actual team membership. A response that it will finish work first is not an assertion that it has joined. Revalidate the invitation and ordinary team eligibility at the eventual join boundary. Use normal refusal, expiry, and team cleanup, without holding stale invitation state indefinitely or modifying ordinary human invitation rules just to make a demonstration pass.

Exact wording, channel, response deadline, delayed-join consent/renewal behavior, and personality-dependent phrasing were not chosen. Resolve these against the real client/team protocol before the affected implementation ticket is Ready. The existing team service rejects an invitation when a reply arrives more than 60 seconds after creation; it sends the inviter a rejection packet and removes the invitation. This is a reply-time check, not proof of automatic expiry cleanup. Do not presume that an invitation remains valid for the duration of a trade delivery. Preserve both agreed outcomes—finish delivery first and explicitly respond—without promising an impossible delayed join.

## Testing decisions

### Highest useful verification seam

Use the existing production PlayerBot runtime and native party/gameplay boundaries, exercised by the existing scenario/bridge and isolated integration facilities. Drive a normal enrolled bot's choice and invitation handling; observe its ordinary action results, resulting team membership, inventory/resources, and the response delivered to the inviter. Do not select the activity through a manual one-off command when claiming independent choice.

Existing decision/scenario rigs and actor tests provide focused deterministic prior art for legal choices and ordinary action consequences. The party decision rig checks real team membership after acceptance; the economy decision rig checks inventory and money after a purchase and rejects unaffordable candidates. These use seeded fixture state, not an independently choosing native bot or a human inviter. Their current coverage does not establish individual motivation, activity-specific commitment, delayed invitation handling, or a client-visible conversational response.

The existing native party follow/assist integration hook exercises the scenario bridge, provisions two bots, and checks follow/target behavior and team membership. Reuse that integration facility, but do not mistake its bot-to-bot scripted scenario for the required human-invitation behavior. Extend the checks to the ordinary human invitation entry and inviter-visible response, while allowing the production bot to choose independently. No new public verification API, generic test framework, or scenario runner is required by this specification.

### Acceptance scenarios

1. **Known procedure, real opportunity:** supply activity know-how and disclose setup resources. The bot selects a supported eligible target and executes ordinary work. An unavailable target or unmet prerequisite is refused/deferred without rewards or false completion. This does not claim that the bot earned the setup.
2. **Individual choice:** under the same supported opportunity and resource conditions, controlled dominant/mixed motivation profiles can produce their intended differing choices. Check the chosen activity and its result, not an internal numeric score or a fixed incidental weighting formula.
3. **Preference is not profit maximization:** with sufficient resources for both alternatives, a bot can choose a preferred lower-return activity. A separate resource-shortage case prevents an unaffordable action; do not grant supplies to preserve the preferred choice during the run.
4. **Economic influence:** with motivations held constant, controlled personal-resource changes and relevant market changes each influence the applicable decision as defined by the bounded policy. Use actual supported data sources and disclose stale/unavailable data cases. Do not require every economic signal to change every bot's decision.
5. **Commitment against rescoring:** introduce a more attractive ordinary opportunity while work is active. The bot retains the current owner to its declared boundary, without duplicate actions or abandoned resources, then reconsiders fresh work.
6. **Trade invitation:** invite a bot already carrying a pack. Observe an explicit player-facing response; if it chooses to join, ordinary delivery occurs before team membership. Check pack ownership and applicable transaction effects, and distinguish delivery success from merely reaching the destination. Exercise invitation expiry/refusal without forcing membership.
7. **Grinding invitation:** invite a grinding bot during its current fight. Observe its response and completion of that fight before an eligible join, without waiting for the whole grind session, forcing a kill, or starting duplicate combat requests.
8. **Decline:** exercise a policy-supported refusal. The inviter receives an explicit response, the bot does not join, and its existing activity and resources remain consistent. A server-side log alone cannot pass this case.
9. **Completion and next choice:** after actual completion or bounded failure/abandonment, the existing owner releases correctly and the bot chooses subsequent eligible work from fresh facts. A new request for the same unfinished task is not a distinct subsequent goal.
10. **Regional reuse:** demonstrate shared policy with declared region/race/item/content differences through valid local targets. Record what was reused and what was reverified; unsupported variants remain named and no whole-world completion claim follows.
11. **Relevant restart/cleanup:** where a child adds durable motivation or commitment state, verify normal save/restart reconciliation, stale invitation/request refusal, and no duplicated costs, rewards, or membership. Do not impose a new persistence mechanism on slices that do not need one.

For each bounded implementation, declare the initial state, supported denominator, ordinary services, trigger, expected consequence, failure/interruption boundary, follow-up choice, and required persistence. Keep focused deterministic, seeded native, earned acquisition-to-repeat, and human/client evidence separate. Trace/packet observations and client presentation have their own scope; H remains unknown without the named human run.

Run the smallest behavior check during authorized development, then required stable-candidate qualification and independent review before integration. Source changes that invoke archaeology retain the repository's archaeology-cycle and normal-gate requirements before merge. No build, gameplay acceptance scenario, or live/client run is claimed by publication of this document.

## Out of scope

- Implementing this parent specification as one task, assigning executable work through publication, or taking over the active recovery check.
- Restricting the product to a settlement, one farm, a fixed occupation, a particular bot, or a single regional demonstration.
- Autonomous learning of activity procedures, exhaustive human life-history simulation, or mandatory full economy/self-acquisition before a useful supported subloop can be tested.
- Fabricated acquisition, supplied merchant stock presented as earned production, free items/money/rewards, synthetic success, or in-run GM/DB repair to satisfy acceptance.
- Replacing Character, the scheduler, activity/result owners, gameplay services, GOAP architecture, economy, tracker, or verification framework.
- A selected Bartle scoring model, new database schema, universal commitment timer, rage-quitting personality behavior, or an approved exceptional danger/death policy.
- Exhaustive world/quest/combat/market coverage, population-scale qualification, or declaring H, milestones, and capability grades complete.
- Gameplay edits, service starts, database mutation, commits, pushes, upstream publication, or deployment under this specification-publishing task.

## Further notes

### Authority, decomposition, and remaining decisions

This specification records the completed product interview using [domain vocabulary](../../.pi-flow/CONTEXT.md). It does not supersede the active assignment in [CURRENT](../../.omp/state/CURRENT.md#session-resume). The recovery owner, protected gameplay sources, preserved fixtures, paused work, and human services retain their existing boundaries.

Use `/pi-to-tickets` to reconcile existing code/evidence and create bounded outcomes under the existing implementation contract. Decompose by behavior and dependency, not by one ticket per class or an entirely new bot framework. Leave unavailable resources and unresolved product choices explicit; select implementers and independent verifiers only for child tickets with grounded scope. The parent remains Backlog, ready for decomposition rather than agent execution.

Before related implementation, reconcile the know-how/opportunity distinction and individual-choice direction in the appropriate authoritative project-control/roadmap records. Update status/scorecard only with the corresponding planning or exercised evidence boundary; no seven-criterion closure or grade follows from this document. Do not replace the active CURRENT objective or rewrite historical evidence to claim these choices already worked.

Material open decisions are the exact motivation model and economic inputs, exceptional commitment interruptions, and invitation response/expiry/renewal presentation. They do not require repeating the settled interview. Ground each in the existing behavior and resolve only the materially different tradeoff needed by the next bounded ticket.

### Evidence boundary and sources

The source baseline is `26612d5902ff24d860bbf99be7dbcb3f5c3b76e8` with inherited dirty work, not a clean qualification candidate. Source inspection and prior recorded results establish reuse candidates, not a fresh gameplay pass. E132 is the existing developed-capability inventory; E295 is the prior human-observed potato cycle with rough animations, not proof of this choice/invitation specification. The active E307/E308 continuation evidence remains bounded to its recorded development controls and native checkpoint behavior.

Source and test inspection identifies the following reuse boundaries. These are code/textual findings at the baseline above; the listed tests were inspected, not run for this specification.

| Existing boundary | What inspection establishes | What still needs bounded implementation or proof |
| --- | --- | --- |
| [Activity arbitration](../../AAEmu.Game/Core/Managers/Bots/BotGoalArbiter.cs#L128-L193) | Registered modules compete in priority order; the active activity name prevents repeated activation of an unchanged choice. | Individual motivational choice and activity-specific commitment cannot be inferred from a single-active name. |
| [Party decision scenario](../../AAEmu.Game/Core/Managers/Bots/PartyDecisionScenario.cs#L151-L234) | A bounded invite/accept/follow/assist proposal set feeds the existing decision selector and actor actions. | This is scenario prior art, not proof of production human-invitation policy or voluntary refusal. |
| [Ordinary invitation handling](../../AAEmu.Game/Core/Managers/TeamManager.cs#L122-L234) | Invitation creation, explicit rejection, the reply-time 60-second check, team membership, and inviter rejection packets already exist. | Resolve deferred join and player-facing intention without treating an expired invitation as consent. |
| [Party decision rig](../../AAEmu.UnitTests/Game/Core/Managers/Bots/PartyDecisionScenarioRigTests.cs#L90-L120) and [economy decision rig](../../AAEmu.UnitTests/Game/Core/Managers/Bots/EconomyDecisionScenarioRigTests.cs#L57-L123) | Seeded checks assert real service consequences, including joined team, changed inventory/money, and refusal of unaffordable choices. | Seeded checks do not prove autonomous acquisition, market-informed motivation, or human presentation. |
| [Party follow/assist integration hook](../../AAEmu.IntegrationTests/E2e/PartyFollowAssistE2eTests.cs#L38-L144) | An existing isolated-server scenario/bridge facility checks ordinary party follow and assist behavior. | Add the human-invitation and independent-choice observations; this publication executes no such scenario. |

The read-only archaeology MCP was invoked for catalog/source corroboration: `list_sources {}` and `read_file {"path":"AAEmu.Game/Core/Managers/Bots/BotGoalArbiter.cs"}`. Catalog source `game-source` is `/root/aaemu-dev/AAEmu.Game`, version `fork develop`; the read response returned that absolute file path, null `source_id`/`version`, and `truncated=false`, with all 9,339 bytes read. Catalog/source evidence is textual/code, not live/client/H. Canonical database MD5 was observed as `78b3bdbf038db3b927056106efdf91af`, version `1.2 r208022`; no gameplay table values or data-dependent item IDs are selected here. See the [archaeology acceptance dossier](../../scorecard-explorations/mechanics/archaeology-mcp-acceptance.md) for layer/provenance limits. Graph output and new human trace capture are not prerequisites for this parent publication; neither is claimed as exercised evidence.

Publication verification is a revision-checked same-origin tracker write followed by a fresh GET comparing the saved parent card and linked specification reference. It is documentation/control evidence only. Independent gameplay review, implementation, deployment, live acceptance, and human acceptance are pending.

### Related

- [Supported-capability contract](../../PROJECT-CONTROL.md#playerbot-supported-capability-contract)
- [Reuse-first integration direction](../../PROJECT-CONTROL.md#playerbot-integration-direction)
- [Developed capability baseline](../../ROADMAP.md#current-developed-capability-baseline)
- [Activity/choice ownership boundaries](../../ROADMAP.md#separate-the-shared-spine-from-activity-policy)
- [Discovery-to-delivery implementation contract](../../.kanban-templates/implementation.md)
- [Development and verification workflow](PlayerBot-Development-Workflow-Spec.md)
- [Delivery & Sessions publishing workflow](../../Scripts/playertrace-coverage/README.md#delivery--sessions-workflow)
- [Evidence ledger](../../.omp/state/EVIDENCE.md)
