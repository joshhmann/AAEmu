# ZONE / INSTANCE AUDIT TEMPLATE — content coverage

> **NEVER push a branch or open a PR to upstream AAEmu/AAEmu.** Upstream is
> intake-only; all reports and follow-up work stay on joshhmann/AAEmu.

Use this for geographically scoped content. Use `explorer.md` for a global
mechanic such as trials, prison, PvP, auction, combat, or housing behavior.

## Identity and scope

- **Canonical key:** main-world zone-group ID + localized name + all member
  zone keys, or exact `Data/Worlds/instance_*` directory key
- **World / adjacent zones:**
- **Milestone or route:**
- **Global mechanic IDs exercised:** from `SCORECARD.md`
- **Commit and data revision inspected:**

## Evidence inventory

For every claim, record the compact table/query or JSON path, code file:line,
and runtime/test artifact. Use `U` rather than guessing.

| Surface | Grade | Canonical inventory | Runtime wiring | Validation / defect |
|---|:---:|---|---|---|
| Quest chains and rewards | U | | | |
| NPC spawns and services | U | | | |
| Doodad spawns, phases, interactions | U | | | |
| Merchants, workstations, public facilities | U | | | |
| Spheres, area triggers, portals | U | | | |
| Transfers and route connections | U | | | |
| Terrain, navigation, water, spawn-Z | U | | | |
| Human route from reproducible reset | U | | | |
| Logout/restart/crash recovery | U | | | |

Grades use the SCORECARD evidence scale: `U`, `0`, `1`, `2`, or justified
`N/A`. A zone is not complete because its JSON loads or because one quest
passes; all surfaces required by its milestone must reach `2`.

## Required passes

1. Inventory canonical zone rows and world JSON; reconcile IDs and names.
2. Use Graphify `explain`, `affected`, and focused `query` calls for the
   managers/packets/models actually reached by this zone.
3. Run an on-foot human route and capture every blocker before using GM repair.
4. Repeat the route segment after logout and server restart.
5. Separate global mechanic defects from local content defects. Link global
   defects to their mechanic ID; do not copy the same bug into every zone.
6. Update the zone ledger row in `SCORECARD.md` and file bounded follow-up
   cards ordered by golden-route impact, corruption/duplication risk, then
   cosmetic completeness.

## Deliverables

- `scorecard-explorations/zones/<zone-key>.md`
- Updated zone ledger evidence links/grades
- Fix/feature cards with mechanic IDs, zone key, repro, owner, and exit test
- One-line tracking handoff; push only to the fork
