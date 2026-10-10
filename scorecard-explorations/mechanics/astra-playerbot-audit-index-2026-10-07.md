# PlayerBot audit index

The [OMP loop handoff](astra-playerbot-omp-loop-handoff-2026-10-07.md) adds verified `/loop` syntax, a bootstrap message, one-card checkpoints and a CURRENT-based stop condition. No loop or lane has been launched.

Author: Astra. Date: 2026-10-07.

All 14 subsystem passes are saved, followed by the final synthesis. The eleven remaining subsystem passes are layers 4–14. Reports use the requested A–M structure and carry the cumulative cross-layer notebook.

**Overall finding: STRUCTURAL-ISSUE.** Useful engine adapters and narrow proofs exist. Continuous autonomous player behavior still depends on authoritative opportunity observation, one intent owner, consistent asynchronous execution and normal gameplay parity.

Read the [final synthesis](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-15-synthesis-2026-10-07.md) for the architecture, five priority changes and realistic field tests. The [working audit protocol](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-audit-protocol-2026-10-07.md) preserves the requested scope and method.

The [P0–P6 rework path](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-rework-path-p0-p6-2026-10-07.md) turns the findings into ordered delivery direction. Each subsystem report now includes section N with its first outcome, canonical owner, dependencies, live stopping point, preserved code and deferred breadth. The phase plan is recommended direction; implementation and acceptance remain pending.

The [Muse handoff](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-muse-handoff-2026-10-07.md) supplies a copyable first-task brief, evidence anchors for every layer, ownership decisions, scope limits and acceptance/stop rules. Begin with the bounded enrollment review; implementation follows the task actually assigned.

| Layer | Report | Verdict |
|---|---|---|
| 1 | [Scheduler, runtime and actor lifecycle](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-01-lifecycle-2026-10-07.md) | STRUCTURAL-ISSUE |
| 2 | [Observation, perception and world-state](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-02-observation-2026-10-07.md) | STRUCTURAL-ISSUE |
| 3 | [Goal arbitration and top-level ownership](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-03-arbitration-2026-10-07.md) | STRUCTURAL-ISSUE |
| 4 | [Quest interpretation](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-04-quest-interpretation-2026-10-07.md) | HEALTHY-BUT-NARROW |
| 5 | [Quest execution](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-05-quest-execution-2026-10-07.md) | STRUCTURAL-ISSUE |
| 6 | [Travel and navigation](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-06-travel-2026-10-07.md) | STRUCTURAL-ISSUE |
| 7 | [Combat](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-07-combat-2026-10-07.md) | NEEDS-HARDENING |
| 8 | [Death, corpse, loot and ownership](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-08-death-loot-2026-10-07.md) | STRUCTURAL-ISSUE |
| 9 | [Interaction primitives](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-09-interactions-2026-10-07.md) | STRUCTURAL-ISSUE |
| 10 | [Survival, recovery and needs](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-10-survival-2026-10-07.md) | STRUCTURAL-ISSUE |
| 11 | [Acquisition, economy, homestead and GOAP](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-11-acquisition-economy-2026-10-07.md) | STRUCTURAL-ISSUE |
| 12 | [Persistence, dormancy and restart](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-12-persistence-2026-10-07.md) | STRUCTURAL-ISSUE |
| 13 | [Shared-world and multi-bot semantics](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-13-shared-world-2026-10-07.md) | STRUCTURAL-ISSUE |
| 14 | [Harness, bridge and evidence](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-14-harness-2026-10-07.md) | STRUCTURAL-ISSUE |
| 15 | [Final synthesis](/root/aaemu-dev/scorecard-explorations/mechanics/astra-playerbot-layer-15-synthesis-2026-10-07.md) | STRUCTURAL-ISSUE |

Baseline: `e6e401129ffe4b5a3bb37f9d0ea521ee40f1e28d` plus the pre-existing dirty working tree. These are source findings and proposed changes. No gameplay edits, new gate/test/live runs, commits, pushes, deployments, project-grade changes or human signoff are part of this audit.

Checks performed for this delivery: report structure, local link targets/line bounds, and the saved report index. Existing unrelated working-tree changes were preserved.
