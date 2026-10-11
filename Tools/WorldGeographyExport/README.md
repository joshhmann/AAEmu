# World geography extraction

This offline tool reads existing ArcheAge 1.2 client files without starting a game
server or changing the archive. It extracts only main-world `world.xml`, root
housing/subzone XML and packaged zone editor layers. Limits: 1,000 files, 8 MiB
per entry, 64 MiB total. The output directory must be empty.

```bash
dotnet run --project Tools/WorldGeographyExport -c Release -- \
  /path/to/game_pak /path/to/empty/geography-input
python3 Scripts/playertrace-coverage/prepare_world_geography.py \
  --extracted /path/to/empty/geography-input \
  --output .client_files/dashboard-map/geography.json
```

The second command reads the canonical `compact.sqlite3` in read-only mode and
joins configured NPC placements with template names, levels and service flags.
It refuses a database with a different recorded MD5. Prepared output is local
and ignored by Git; do not commit the archive, extracted XML or client artwork.

Read [World geography and routes](../../Docs/wiki/World-Geography-and-Routes.md)
for the evidence boundaries and route-data contract.
