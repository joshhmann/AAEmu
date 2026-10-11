# World geography and routes

The atlas can show named areas, housing polygons, editor road shapes, configured
NPC services and other configured NPC placements. Enable the layers in **World
Map & Atlas**. Click a place to see its source and what that source can establish.
NPC dots appear at closer zoom levels; purple dots show configured services.
Missing spawn altitude stays unknown; it is never replaced with a guessed height.

Prepare the data with [WorldGeographyExport](../../Tools/WorldGeographyExport/README.md).
The normal dashboard reads `.client_files/dashboard-map/geography.json` through
`/api/map/geography`; it does not need direct archive or database access. Without
prepared data, other map features continue working. Oversized output is rejected
at 32 MiB. Image assets still come from the existing map preparation workflow.

## What the map tells us

The area coordinate transform follows `SubZoneManager`: point plus entity
position plus world-zone origin and entity cell offsets, with 1,024 metres per
cell. Zone key, zone ID and group ID remain separate. Housing rule matches are
reported by ID; an unmatched shape stays unmatched.

A housing boundary does not prove an empty plot or permission to place a house.
A town name does not prove PvP protection. A configured NPC placement does not
prove the NPC is alive, available, hostile or reachable. The server owns those
answers. Root area files are included; locale-specific overrides are excluded
and explicitly reported in coverage.

Editor road transforms remain provisional, particularly rotation/scale. Orange
road lines are reference candidates and cannot become executable routes just
because they were extracted. Source entry hashes and the reference database MD5
are retained in the prepared data. The client version is an asset label, not a
newly verified client build identity.

## How usable routes are represented

`RoadNetworkService` remains the road-network owner. Its original
`FindRoadPath` API is retained for inspecting the candidate junction graph.
`FindTravelRoute` accepts full evidenced walking geometry and an explicit
character/world decision context. It includes approach and departure distances,
all bends, direction and nonnegative additional time costs. Unknown additional
cost withdraws an edge. It does not infer safety from static geography.

A usable edge in `map_atlas_data.json` requires:

- `world_id` on both junctions and the edge, with matching values.
- `travel_mode: "walk"`; other modes remain unsupported.
- A nonempty `route` ID and `navigation_evidence` reference to the actual proof.
- `points`, containing finite XYZ triples and terminating at the exact junctions
  (within 0.01 metres of serialization precision).
- Explicit `bidirectional: true` if reverse traversal was verified; otherwise
  the edge is one-way.
- `zone_keys` where known. These identify regions; they do not declare them safe.

The evidence reference is a trust contract for reviewed route data, not a proof
validator: an arbitrary string does not certify a road. No existing candidate
edge was promoted in this change. Archive-derived roads never populate this
executable graph automatically. The game embeds the atlas during its build;
changing a dashboard file does not update an already built server.

`RoadTravelQuery` supplies world/instance identity, actual supported speed,
observed extra costs and ordinary-navigation connectors. A nearby junction alone
cannot establish a connector. Connector geometry and extra cost must be supplied
explicitly; only coincident endpoints need no approach. Connector search is
bounded to the nearest eight candidates within at most 2,000 metres, using the
existing spatial buckets. Geometry is cached at startup rather than rediscovered
on each wake. Unknown danger must not be passed as a known zero.

## Connection to travel

`TravelBrainPlanner.Prepare` accepts the route context only for a known
same-world, same-instance position destination. It preserves the full polyline
in the existing `TravelIntentStore`. A changed relevant observation revision is
reconsidered after the current movement leg finishes. It retains the destination,
recovery counters and owning leg; it does not replace a live actor request or
reset the journey. Immediate combat/survival interruption remains with existing
reactive behaviors.

**Current runtime boundary:** current combat, loot and quest travel callers use
unit/local movement and set `AllowRoute: false`. They do not supply this new
route context. Existing atlas edges have no verified geometry. Therefore this
change provides tested route selection and its travel integration, but does not
activate risk-aware road travel for live bots. Ordinary movement continues.
There is no new brain, scheduler, transport simulation or goal-selection owner.

## Evidence and the next journey

The isolated implementation starts at `aa057ffc3bb4596f4a6972eb4f012994970e163a`.
The bounded extraction in this environment found 181 root area files, two editor
layers, 380 housing shapes, 920 subzone shapes and 45 road objects. The source snapshot contains 24,421 configured NPC records, including 261 with
no altitude supplied. These remain visible in two dimensions with unknown Z.
Counts are coverage of these inputs, not a whole-world or live population claim.

Unit tests use synthetic evidenced topology and explicitly supplied danger and
connector observations. They establish geometry, cost, world/direction checks,
unknown handling and intent preservation. Browser checks establish rendering,
source inspection and functioning controls. Neither proves live navigation.
Archaeology source/data corroboration follows the existing
[acceptance dossier](../../scorecard-explorations/mechanics/archaeology-mcp-acceptance.md).
Exact commands, hashes and results are saved alongside the isolated checkout.
No client/H, live journey, merge or deployment is claimed.

Next, verify one real walking corridor with two usable alternatives through
ordinary navigation/movement, including approach, junction joins and departure.
Record which world/instance and direction were actually walked. Bind the route
context to authoritative relevant world observations rather than supplied test
penalties. During a second trip, change a real obstruction or observed danger;
the bot must retain its destination, use the supported alternate path and arrive
without teleport, injected completion or manual movement driving. A road-drawing
fixture or a mocked cost change cannot close that proof.

## Quest givers and objective areas

Enable **Quest givers**, then click a yellow NPC dot to list its quests. Search by
quest name or ID to select a quest directly. **Locate selection** opens matching
zone artwork around a known placement. The selected quest shows green turn-in
placements, red target placements and blue client component circles. Clicking a
completed-quest reference selects that related quest. **Clear selection** removes
these highlights without changing a developer route or live radar.

NPC quest links come from typed accept/report acts, not from every NPC or the
mostly empty `quest_components.npc_id`. Hunt and talk groups expand through their
actual membership tables. Doodad interaction markers use configured placements;
required phase, availability and legality remain ordinary server decisions.
All objective acts are retained, including objectives with no mapped location.
Item sources and dynamically spawned targets are explicitly unresolved.

The client supplies `quest_sign_sphere.g` files. Their positions use the server's
zone origin transform (origin cell × 1,024 metres plus local position). Like the
server, the exporter attaches geometry by component ID; raw client quest IDs may
be stale. Blue circles represent the client component marker, not an invented
boundary containing every eligible target. NPC-relative sphere triggers are not
shown as fixed circles. Completed-quest requirements preserve their component's
AND/OR setting and other start conditions; they do not prove a quest is currently
available or form a single linear storyline. Within-quest `next_component` links
are never presented as links to another quest.

Build the additional reference data after preparing geography and map images:

```bash
dotnet run --project Tools/WorldGeographyExport -c Release -- \
  /path/to/game_pak /path/to/empty/quest-area-input --quest-areas
python3 Scripts/playertrace-coverage/prepare_quest_atlas.py \
  --quest-areas /path/to/empty/quest-area-input
```

This reads the canonical database without writing it and saves local
`.client_files/dashboard-map/quests.json`. The dashboard serves it through
`/api/map/quests` with a 32 MiB bound. Missing data leave other map controls usable.
The extracted reference files and prepared JSON stay out of Git.

Coverage of the supplied sources: 4,876 quests, 1,744 giver templates, 2,633
configured giver placements, 3,848 objective acts, 2,258 acts with mapped
placements or circles, and 2,453 raw client area records. Raw area records may
include components outside the current quest table; their total is not a claim
that every area becomes a selected quest's objective. No PlayerBot quest
selection, completion behavior or gameplay service is changed.

For road mapping, a visual match to the dark brown paths in the zone artwork is
useful evidence. NPC placements, named areas and other map assets help check the
alignment and infer connections. Walking each road is not a prerequisite for
displaying or reviewing that map layer. The separate claim that a bot can travel
it still needs ordinary server movement to succeed; a map image cannot establish
collision clearance, bridge transitions or current danger.
